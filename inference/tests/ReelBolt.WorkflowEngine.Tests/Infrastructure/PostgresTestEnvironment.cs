#nullable enable
using System;
using Npgsql;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Infrastructure;

/// <summary>
/// The one place the Postgres-gated tests get their connection strings. The base server comes from
/// <c>REELBOLT_TEST_PG</c>, else <c>REELBOLT_TEST_DATABASE_URL</c> (an Npgsql connection string with
/// CREATEDB rights); without either, every gated test returns early or skips. Each per-test database
/// gets a capped pool so the whole suite fits a server's <c>max_connections</c>;
/// <c>inference/tests/run-postgres-tests.sh</c> runs them on a throwaway server.
/// </summary>
public static class PostgresTestEnvironment
{
    public const string PrimaryVariable = "REELBOLT_TEST_PG";
    public const string FallbackVariable = "REELBOLT_TEST_DATABASE_URL";

    /// <summary>Pool cap of a per-test database unless a race needs more connections open at once.</summary>
    public const int DefaultMaxPoolSize = 10;

    /// <summary>Pool cap of the admin source that only creates and drops databases.</summary>
    public const int AdminMaxPoolSize = 2;

    /// <summary>The base connection string, or <c>null</c> when no Postgres is configured.</summary>
    public static string? BaseConnectionString()
    {
        string? primary = Environment.GetEnvironmentVariable(PrimaryVariable);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        string? fallback = Environment.GetEnvironmentVariable(FallbackVariable);
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <summary>
    /// Why the real-Postgres tests are skipped, or <c>null</c> when <see cref="BaseConnectionString"/> finds a server.
    /// <see cref="ReelBolt.WorkflowEngine.Tests.PostgresFactAttribute"/> skips at discovery with this message, so a run
    /// that never reached Postgres shows those tests as skipped instead of counting an early <c>return</c> as a pass.
    /// </summary>
    public static string? SkipReason =>
        BaseConnectionString() is null
            ? $"Needs a real PostgreSQL: set {PrimaryVariable} (or {FallbackVariable}) to a server connection string "
              + "(e.g. Host=127.0.0.1;Port=55432;Username=postgres;Password=postgres)."
            : null;

    /// <summary>The base connection string pointed at <paramref name="database"/>, its pool capped at <paramref name="maxPoolSize"/>.</summary>
    public static string ForDatabase(string baseConnectionString, string database, int maxPoolSize = DefaultMaxPoolSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoolSize, 1);
        return new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = database, MaxPoolSize = maxPoolSize }.ConnectionString;
    }

    /// <summary>A data source on the base database for CREATE/DROP DATABASE, capped at <see cref="AdminMaxPoolSize"/>.</summary>
    public static NpgsqlDataSource CreateAdminDataSource(string baseConnectionString)
        => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(baseConnectionString) { MaxPoolSize = AdminMaxPoolSize }.ConnectionString);
}

/// <summary>
/// Every Postgres-gated class joins this collection, so they run one after another rather than each
/// opening its pools against the same server at once.
/// </summary>
[CollectionDefinition("Postgres")]
public sealed class PostgresCollection
{
}
