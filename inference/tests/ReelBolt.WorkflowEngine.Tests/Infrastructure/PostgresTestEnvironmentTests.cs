#nullable enable
using System;
using AwesomeAssertions;
using Npgsql;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Infrastructure;

/// <summary>The connection helper the Postgres-gated tests share; no server needed.</summary>
public sealed class PostgresTestEnvironmentTests
{
    private const string Base = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=pw;Maximum Pool Size=100";

    [Fact]
    public void ForDatabase_points_at_the_database_and_caps_the_pool_at_the_default()
    {
        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(Base, "reelbolt_x"));

        csb.Database.Should().Be("reelbolt_x");
        csb.MaxPoolSize.Should().Be(10).And.Be(PostgresTestEnvironment.DefaultMaxPoolSize);
        csb.Host.Should().Be("127.0.0.1");
        csb.Password.Should().Be("pw");
    }

    [Fact]
    public void ForDatabase_honours_an_explicit_pool_size_and_rejects_a_non_positive_one()
    {
        new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(Base, "reelbolt_x", maxPoolSize: 3)).MaxPoolSize.Should().Be(3);

        Action zero = () => PostgresTestEnvironment.ForDatabase(Base, "reelbolt_x", maxPoolSize: 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_admin_source_stays_on_the_base_database_with_a_pool_of_two()
    {
        using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(Base);
        NpgsqlConnectionStringBuilder csb = new(admin.ConnectionString);

        csb.Database.Should().Be("postgres");
        csb.MaxPoolSize.Should().Be(2);
    }
}
