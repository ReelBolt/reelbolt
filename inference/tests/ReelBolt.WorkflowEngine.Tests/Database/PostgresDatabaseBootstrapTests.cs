#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// Security S-1 on the create-database path: <c>MigrateAsync</c> on a missing database makes EF open an
/// admin connection on an unnamed data source, whose <c>db.client.connection.pool.name</c> is the
/// connection string minus <c>Password</c> (host, user, <c>SSL Password</c>). Both services call
/// <see cref="PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync"/> first, so the database already
/// exists and EF never takes that path. Each test mints and drops its own database.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class PostgresDatabaseBootstrapTests
{
    private const string PoolNameTag = "db.client.connection.pool.name";
    private const string ApiDataSourceName = "reelbolt-inference-api";
    private const string BootstrapName = "reelbolt-inference-api-bootstrap";
    private const string CreatedMessage = "created";

    [Fact]
    public async Task Migrating_a_missing_database_after_the_bootstrap_never_publishes_the_ssl_password()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = NewDatabaseName();
        // Unique per test, so a pool another test opened in this process can never match it (Security S-19).
        string sslMarker = NewSslMarker();
        string connectionString = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName))
        {
            SslPassword = sslMarker,
        }.ConnectionString;
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);

        try
        {
            var during = new List<string>();
            using (MeterListener listener = StartPoolNameListener(during))
            {
                await PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapName, new RecordingLogger());

                DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseNpgsql(connectionString, npgsql => npgsql
                        .MigrationsHistoryTable("__EFMigrationsHistory_Api")
                        .ConfigureDataSource(dataSource => dataSource.Name = ApiDataSourceName))
                    .Options;
                await using (var db = new InferenceApiDbContext(options))
                {
                    await db.Database.MigrateAsync();
                }

                listener.RecordObservableInstruments();
            }

            (await DatabaseExistsAsync(admin, dbName)).Should().BeTrue();

            // Only this test's own marker and names are asserted on: other classes in the same process
            // (BillingChangeLockConnectionTests, ...) may publish their own fixed pool names meanwhile.
            string[] observed = Snapshot(during);
            observed.Should().NotContain(name => name.Contains(sslMarker, StringComparison.Ordinal),
                "no pool name may carry this test's SSL Password, i.e. EF's unnamed admin source never ran");
            observed.Should().Contain(BootstrapName, "the bootstrap probed the target through its named source");
            observed.Should().Contain(BootstrapName + "-admin", "the bootstrap, not EF, created the database");
        }
        finally
        {
            await DropDatabaseAsync(admin, dbName);
        }
    }

    // Npgsql 11 + EF provider 10.0.4: EF's own create path throws MissingMethodException, so the database
    // must exist as soon as the bootstrap returns, before any MigrateAsync (docs/database-connections.md).
    [Fact]
    public async Task The_bootstrap_creates_a_missing_database_before_any_migration_runs()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = NewDatabaseName();
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);

        try
        {
            (await DatabaseExistsAsync(admin, dbName)).Should().BeFalse();
            await PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(
                PostgresTestEnvironment.ForDatabase(baseConnection, dbName), BootstrapName, new RecordingLogger());
            (await DatabaseExistsAsync(admin, dbName)).Should().BeTrue();
        }
        finally
        {
            await DropDatabaseAsync(admin, dbName);
        }
    }

    // Security S-16 / QA-13: when the database is missing and the bootstrap cannot create it (no CREATEDB),
    // startup fails with the step and SqlState; it no longer logs and leaves the create to EF's unnamed source.
    [Fact]
    public async Task A_role_without_createdb_on_a_missing_database_throws_and_never_publishes_the_ssl_password()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = NewDatabaseName();
        string role = "reelbolt_bootstrap_nocreatedb_" + Guid.NewGuid().ToString("N")[..12];
        string rolePassword = "rolepw-" + Guid.NewGuid().ToString("N");
        string sslMarker = NewSslMarker();
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand createRole = admin.CreateCommand(
            $"CREATE ROLE \"{role}\" LOGIN NOCREATEDB NOSUPERUSER PASSWORD '{rolePassword}'"))
        {
            await createRole.ExecuteNonQueryAsync();
        }

        try
        {
            string connectionString = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName))
            {
                Username = role,
                Password = rolePassword,
                SslPassword = sslMarker,
            }.ConnectionString;

            var during = new List<string>();
            InvalidOperationException failure;
            using (MeterListener listener = StartPoolNameListener(during))
            {
                Func<Task> act = () => PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapName, new RecordingLogger());
                failure = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
                listener.RecordObservableInstruments();
            }

            failure.Message.Should().Contain("42501");
            failure.Message.Should().Contain("create");
            failure.Message.Should().Contain(BootstrapName + "-admin");
            failure.Message.Should().NotContain("Password");
            failure.Message.Should().NotContain(rolePassword);
            failure.Message.Should().NotContain(sslMarker);
            failure.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be("42501");
            Snapshot(during).Should().NotContain(name => name.Contains(sslMarker, StringComparison.Ordinal));
            (await DatabaseExistsAsync(admin, dbName)).Should().BeFalse();
        }
        finally
        {
            await DropDatabaseAsync(admin, dbName);
            await using NpgsqlCommand dropRole = admin.CreateCommand($"DROP ROLE IF EXISTS \"{role}\"");
            await dropRole.ExecuteNonQueryAsync();
        }
    }

    // Security S-25: a string with neither Database nor Username. Npgsql 11.0.0 takes the user from PGUSER
    // (NpgsqlConnector.GetUsernameAsync) and sends no database, so the server opens the database named
    // after that user; PGDATABASE is never read (PostgresEnvironment has no such key). The bootstrap must
    // create that database instead of returning, or MigrateAsync takes EF's unnamed admin source.
    // PGUSER/PGDATABASE are process-wide: the Postgres collection keeps every gated test off them meanwhile.
    [Fact]
    public async Task A_string_without_database_or_username_creates_the_database_npgsql_resolves_from_pguser()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        // The role and its default database share one name; PGDATABASE names a decoy Npgsql must ignore.
        string role = NewDatabaseName();
        string decoy = NewDatabaseName();
        string rolePassword = "rolepw-" + Guid.NewGuid().ToString("N");
        string sslMarker = NewSslMarker();
        var builder = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Password = rolePassword,
            SslPassword = sslMarker,
            MaxPoolSize = PostgresTestEnvironment.DefaultMaxPoolSize,
        };
        builder.Remove("Database");
        builder.Remove("Username");
        string connectionString = builder.ConnectionString;

        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand createRole = admin.CreateCommand(
            $"CREATE ROLE \"{role}\" LOGIN CREATEDB NOSUPERUSER PASSWORD '{rolePassword}'"))
        {
            await createRole.ExecuteNonQueryAsync();
        }

        string? previousUser = Environment.GetEnvironmentVariable("PGUSER");
        string? previousDatabase = Environment.GetEnvironmentVariable("PGDATABASE");
        try
        {
            Environment.SetEnvironmentVariable("PGUSER", role);
            Environment.SetEnvironmentVariable("PGDATABASE", decoy);

            var during = new List<string>();
            Exception? migrateFailure = null;
            using (MeterListener listener = StartPoolNameListener(during))
            {
                await PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapName, new RecordingLogger());

                DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseNpgsql(connectionString, npgsql => npgsql
                        .MigrationsHistoryTable("__EFMigrationsHistory_Api")
                        .ConfigureDataSource(dataSource => dataSource.Name = ApiDataSourceName))
                    .Options;
                try
                {
                    await using var db = new InferenceApiDbContext(options);
                    await db.Database.MigrateAsync();
                }
                catch (Exception ex)
                {
                    migrateFailure = ex;
                }

                listener.RecordObservableInstruments();
            }

            Snapshot(during).Should().NotContain(name => name.Contains(sslMarker, StringComparison.Ordinal),
                "no pool name may carry this test's SSL Password, i.e. EF's unnamed admin source never ran");
            (await DatabaseExistsAsync(admin, role)).Should().BeTrue("the bootstrap created the database Npgsql connects to");
            (await DatabaseExistsAsync(admin, decoy)).Should().BeFalse("Npgsql never reads PGDATABASE");
            migrateFailure.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGUSER", previousUser);
            Environment.SetEnvironmentVariable("PGDATABASE", previousDatabase);
            await DropDatabaseAsync(admin, role);
            await DropDatabaseAsync(admin, decoy);
            await using NpgsqlCommand dropRole = admin.CreateCommand($"DROP ROLE IF EXISTS \"{role}\"");
            await dropRole.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Two_concurrent_calls_on_a_missing_database_both_succeed()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = NewDatabaseName();
        string connectionString = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        var first = new RecordingLogger();
        var second = new RecordingLogger();

        try
        {
            await Task.WhenAll(
                Task.Run(() => PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapName, first)),
                Task.Run(() => PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapName, second)));

            (await DatabaseExistsAsync(admin, dbName)).Should().BeTrue();
            first.Entries.Concat(second.Entries).Should().NotContain(e => e.Level >= LogLevel.Warning);
            first.Entries.Concat(second.Entries).Count(e => e.Message.Contains(CreatedMessage, StringComparison.Ordinal))
                .Should().Be(1, "exactly one of the two calls creates the database");
        }
        finally
        {
            await DropDatabaseAsync(admin, dbName);
        }
    }

    [Fact]
    public async Task A_call_on_an_existing_database_is_a_no_op()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = NewDatabaseName();
        string connectionString = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE \"{dbName}\""))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using (NpgsqlDataSource target = NpgsqlDataSource.Create(connectionString))
            await using (NpgsqlCommand marker = target.CreateCommand("CREATE TABLE bootstrap_marker (id int); INSERT INTO bootstrap_marker VALUES (7);"))
            {
                await marker.ExecuteNonQueryAsync();
            }

            var logger = new RecordingLogger();
            await PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapName, logger);

            logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
            logger.Entries.Should().NotContain(e => e.Message.Contains(CreatedMessage, StringComparison.Ordinal));
            await using (NpgsqlDataSource target = NpgsqlDataSource.Create(connectionString))
            await using (NpgsqlCommand read = target.CreateCommand("SELECT id FROM bootstrap_marker"))
            {
                (await read.ExecuteScalarAsync()).Should().Be(7);
            }
        }
        finally
        {
            await DropDatabaseAsync(admin, dbName);
        }
    }

    private static string NewDatabaseName() => "reelbolt_bootstrap_test_" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<bool> DatabaseExistsAsync(NpgsqlDataSource admin, string dbName)
    {
        await using NpgsqlCommand exists = admin.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)");
        exists.Parameters.Add(new NpgsqlParameter { Value = dbName });
        return (bool)(await exists.ExecuteScalarAsync())!;
    }

    private static async Task DropDatabaseAsync(NpgsqlDataSource admin, string dbName)
    {
        dbName.Should().StartWith("reelbolt_bootstrap_test_");
        await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    private static string NewSslMarker() => "sslsecret-" + Guid.NewGuid().ToString("N");

    private static string[] Snapshot(List<string> names)
    {
        lock (names)
        {
            return names.Distinct().ToArray();
        }
    }

    /// <summary>Records the pool-name tag of every Npgsql measurement into <paramref name="names"/> until disposed.</summary>
    private static MeterListener StartPoolNameListener(List<string> names)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Npgsql") l.EnableMeasurementEvents(instrument);
        };
        void Collect(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key != PoolNameTag) continue;
                lock (names)
                {
                    names.Add(tag.Value?.ToString() ?? string.Empty);
                }
            }
        }
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) => Collect(tags));
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => Collect(tags));
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => Collect(tags));
        listener.Start();
        return listener;
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
