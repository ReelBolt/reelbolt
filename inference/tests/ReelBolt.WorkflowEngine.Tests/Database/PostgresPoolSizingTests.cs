#nullable enable

using System;
using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ReelBolt.Shared.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// Pool-size precedence (docs/database-connections.md): Database:MaxPoolSize &gt; a Maximum Pool Size
/// already in the connection string &gt; the service default. The result is parsed back with
/// Npgsql's own builder, so a key Npgsql would reject at the first connection fails here instead.
/// </summary>
public class PostgresPoolSizingTests
{
    private static IConfiguration Config(string? maxPoolSize = null)
    {
        var values = new Dictionary<string, string?>();
        if (maxPoolSize is not null) values["Database:MaxPoolSize"] = maxPoolSize;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static NpgsqlConnectionStringBuilder Parse(string cs) => new(cs);

    [Fact]
    public void Default_is_applied_when_neither_config_nor_string_sets_it()
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize(
            "Host=db;Database=reelforge;Username=postgres;Password=postgres", Config(), 20));

        b.MaxPoolSize.Should().Be(20);
        b.Host.Should().Be("db");
        b.Database.Should().Be("reelforge");
        b.Password.Should().Be("postgres");
    }

    [Fact]
    public void Config_wins_over_the_connection_string_and_the_default()
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize(
            "Host=db;Maximum Pool Size=7", Config("12"), 20));

        b.MaxPoolSize.Should().Be(12);
    }

    [Fact]
    public void A_value_already_in_the_connection_string_is_kept_over_the_default()
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize(
            "Host=db;Maximum Pool Size=7", Config(), 20));

        b.MaxPoolSize.Should().Be(7);
    }

    [Theory]
    [InlineData("Max Pool Size")]
    [InlineData("MaxPoolSize")]
    [InlineData("maximum pool size")]
    public void Every_alias_in_the_connection_string_is_recognised_and_kept(string key)
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize(
            $"Host=db;{key}=7", Config(), 20));

        b.MaxPoolSize.Should().Be(7);
    }

    [Theory]
    [InlineData("Max Pool Size")]
    [InlineData("MaxPoolSize")]
    public void Config_replaces_an_alias_rather_than_adding_a_second_key(string key)
    {
        string result = PostgresPoolSizing.ApplyMaxPoolSize($"Host=db;{key}=7", Config("9"), 20);

        Parse(result).MaxPoolSize.Should().Be(9);
        result.Should().NotContainEquivalentOf(key + "=");
    }

    [Fact]
    public void Values_with_separators_survive_the_rebuild()
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize(
            "Host=db;Username=postgres;Password='p;w=x'", Config(), 15));

        b.Password.Should().Be("p;w=x");
        b.MaxPoolSize.Should().Be(15);
    }

    [Fact]
    public void A_blank_config_value_is_treated_as_unset()
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize("Host=db", Config(" "), 15));

        b.MaxPoolSize.Should().Be(15);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void A_non_positive_or_unparsable_config_value_throws_naming_the_key(string value)
    {
        Action act = () => PostgresPoolSizing.ApplyMaxPoolSize("Host=db", Config(value), 20);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Database:MaxPoolSize*");
    }

    // Security S-6: a pool size already in the connection string is validated too, so a bad value
    // fails startup here instead of every open (0) or the first Npgsql use (abc, -5).
    [Theory]
    [InlineData("Maximum Pool Size", "0")]
    [InlineData("Maximum Pool Size", "-5")]
    [InlineData("Maximum Pool Size", "abc")]
    [InlineData("MaxPoolSize", "0")]
    [InlineData("Max Pool Size", "abc")]
    public void A_non_positive_or_unparsable_connection_string_value_throws_naming_the_setting(string key, string value)
    {
        const string connectionString = "Host=secret-host;Username=secret-user;Password=secret-pw";
        Action act = () => PostgresPoolSizing.ApplyMaxPoolSize($"{connectionString};{key}={value}", Config(), 20);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains(key, StringComparison.Ordinal)
                        && e.Message.Contains("connection string", StringComparison.OrdinalIgnoreCase)
                        && !e.Message.Contains("secret", StringComparison.Ordinal)
                        && !e.Message.Contains($"'{value}'", StringComparison.Ordinal));
    }

    // Security T-041 F4: a mis-quoted value can carry the rest of the string, so the error names the
    // keyword and the requirement only and never echoes the value.
    [Fact]
    public void A_mis_quoted_connection_string_value_is_not_echoed_in_the_error()
    {
        Action act = () => PostgresPoolSizing.ApplyMaxPoolSize("Host=db;Maximum Pool Size=\"20;Password=leak\"", Config(), 20);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("Maximum Pool Size", StringComparison.Ordinal)
                        && e.Message.Contains("whole number greater than 0", StringComparison.Ordinal)
                        && !e.Message.Contains("leak", StringComparison.Ordinal)
                        && !e.Message.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // Security T-041 F3: the sampler builds its own data source from the raw string, which Npgsql
    // rejects when it carries the SqlClient spelling; every recognised spelling can be removed.
    [Theory]
    [InlineData("Max Pool Size")]
    [InlineData("Maximum Pool Size")]
    [InlineData("MaxPoolSize")]
    [InlineData("max pool size")]
    [InlineData("MAXIMUM POOL SIZE")]
    public void RemoveMaxPoolSize_drops_every_spelling_and_keeps_the_rest(string key)
    {
        string result = PostgresPoolSizing.RemoveMaxPoolSize($"Host=db;Username=u;Password='p;w';{key}=20");

        result.Should().NotContainEquivalentOf("pool size");
        result.Should().NotContainEquivalentOf("maxpoolsize");
        NpgsqlConnectionStringBuilder b = Parse(result);
        b.Host.Should().Be("db");
        b.Password.Should().Be("p;w");
    }

    // Security S-21 / QA-10: Npgsql 10 rejects the SqlClient spelling "Min Pool Size", so every
    // Minimum spelling is rewritten to the canonical keyword, as the Maximum ones are.
    [Theory]
    [InlineData("Min Pool Size")]
    [InlineData("MinPoolSize")]
    [InlineData("min pool size")]
    [InlineData("MIN POOL SIZE")]
    [InlineData("Minimum Pool Size")]
    public void Every_minimum_spelling_is_rewritten_to_one_the_npgsql_builder_accepts(string key)
    {
        string result = PostgresPoolSizing.ApplyMaxPoolSize($"Host=127.0.0.1;Port=1;Username=u;Password=p;{key}=2", Config(), 20);

        NpgsqlConnectionStringBuilder b = Parse(result);
        b.MinPoolSize.Should().Be(2);
        b.MaxPoolSize.Should().Be(20);
        b.Password.Should().Be("p");
        result.Should().Contain("Minimum Pool Size=2");
    }

    [Theory]
    [InlineData("Min Pool Size")]
    [InlineData("Minimum Pool Size")]
    [InlineData("MinPoolSize")]
    [InlineData("min pool size")]
    public void RemoveMaxPoolSize_also_drops_every_minimum_spelling(string key)
    {
        string result = PostgresPoolSizing.RemoveMaxPoolSize($"Host=db;Username=u;Password='p;w';Max Pool Size=20;{key}=2");

        result.Should().NotContainEquivalentOf("pool size");
        result.Should().NotContainEquivalentOf("minpoolsize");
        NpgsqlConnectionStringBuilder b = Parse(result);
        b.Host.Should().Be("db");
        b.Password.Should().Be("p;w");
    }

    // QA-4 remainder: a Minimum Pool Size above the effective maximum passed startup and failed at
    // the first open; it now fails startup, naming both settings and both numbers and nothing else.
    private const string SecretConnection = "Host=secret-host;Username=secret-user;Password=secret-pw";

    [Theory]
    [InlineData("Minimum Pool Size")]
    [InlineData("Min Pool Size")]
    [InlineData("MinPoolSize")]
    [InlineData("minimum pool size")]
    [InlineData("MIN POOL SIZE")]
    public void A_minimum_above_the_default_maximum_throws_naming_both_settings_and_numbers(string key)
    {
        Action act = () => PostgresPoolSizing.ApplyMaxPoolSize($"{SecretConnection};{key}=30", Config(), 20);

        // Named in its recognised spelling, as the Maximum Pool Size errors are: keys are case-insensitive.
        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains(key, StringComparison.OrdinalIgnoreCase)
                        && e.Message.Contains("Maximum Pool Size", StringComparison.Ordinal)
                        && e.Message.Contains("30", StringComparison.Ordinal)
                        && e.Message.Contains("20", StringComparison.Ordinal)
                        && !e.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_minimum_above_the_configured_maximum_throws_naming_the_config_key()
    {
        Action act = () => PostgresPoolSizing.ApplyMaxPoolSize($"{SecretConnection};Minimum Pool Size=10", Config("5"), 20);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("Minimum Pool Size", StringComparison.Ordinal)
                        && e.Message.Contains("Database:MaxPoolSize", StringComparison.Ordinal)
                        && e.Message.Contains("10", StringComparison.Ordinal)
                        && e.Message.Contains("5", StringComparison.Ordinal)
                        && !e.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_minimum_above_a_maximum_in_the_string_throws_naming_both_keywords()
    {
        Action act = () => PostgresPoolSizing.ApplyMaxPoolSize($"{SecretConnection};MaxPoolSize=8;Min Pool Size=9", Config(), 20);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("Min Pool Size", StringComparison.Ordinal)
                        && e.Message.Contains("MaxPoolSize", StringComparison.Ordinal)
                        && e.Message.Contains("9", StringComparison.Ordinal)
                        && e.Message.Contains("8", StringComparison.Ordinal)
                        && !e.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_minimum_equal_to_the_effective_maximum_is_accepted()
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize("Host=db;Minimum Pool Size=20", Config(), 20));

        b.MaxPoolSize.Should().Be(20);
        b.MinPoolSize.Should().Be(20);
    }

    [Fact]
    public void A_minimum_below_the_effective_maximum_is_accepted()
    {
        NpgsqlConnectionStringBuilder b = Parse(PostgresPoolSizing.ApplyMaxPoolSize("Host=db;Minimum Pool Size=5", Config(), 20));

        b.MaxPoolSize.Should().Be(20);
        b.MinPoolSize.Should().Be(5);
    }

    [Theory]
    [InlineData("Minimum Pool Size", "abc")]
    [InlineData("Min Pool Size", "-1")]
    [InlineData("MinPoolSize", "1.5")]
    public void A_negative_or_unparsable_minimum_throws_naming_the_setting(string key, string value)
    {
        Action act = () => PostgresPoolSizing.ApplyMaxPoolSize($"{SecretConnection};{key}={value}", Config(), 20);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains(key, StringComparison.Ordinal)
                        && e.Message.Contains("connection string", StringComparison.OrdinalIgnoreCase)
                        && !e.Message.Contains("secret", StringComparison.Ordinal)
                        && !e.Message.Contains($"'{value}'", StringComparison.Ordinal));
    }
}
