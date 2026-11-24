#nullable enable

using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Shared.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// Security S-16 / QA-13: a bootstrap that cannot reach the server fails startup instead of logging and
/// returning, so <c>MigrateAsync</c> (and EF's unnamed admin data source) never runs after it. Needs no
/// Postgres: port 1 on loopback refuses the connection. The PG-gated failure (a role without CREATEDB)
/// lives in <see cref="PostgresDatabaseBootstrapTests"/>.
/// </summary>
public class PostgresDatabaseBootstrapFailureTests
{
    private const string BootstrapName = "reelbolt-inference-api-bootstrap";
    private const string PasswordValue = "pw-value-7c1e";

    private static string RefusedConnection(string sslMarker, string extra = "")
        => $"Host=127.0.0.1;Port=1;Database=reelbolt_missing;Username=bootstrap-user;Password={PasswordValue};SSL Password={sslMarker};Timeout=5{extra}";

    [Fact]
    public async Task A_refused_port_throws_naming_the_source_and_step_without_connection_string_text()
    {
        string sslMarker = "sslsecret-" + Guid.NewGuid().ToString("N");

        Func<Task> act = () => PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(
            RefusedConnection(sslMarker), BootstrapName, NullLogger.Instance);

        InvalidOperationException ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.InnerException.Should().BeAssignableTo<NpgsqlException>("the original failure is kept as the InnerException");
        ex.Message.Should().Contain(BootstrapName);
        ex.Message.Should().Contain("probe");
        ex.Message.Should().Contain(ex.InnerException!.GetType().Name);
        ex.Message.Should().NotContain("Password");
        ex.Message.Should().NotContain("SSL Password");
        ex.Message.Should().NotContain(PasswordValue);
        ex.Message.Should().NotContain(sslMarker);
        ex.Message.Should().NotContain("bootstrap-user");
        ex.Message.Should().NotContain("127.0.0.1");
    }

    // Security S-25: neither Database nor Username no longer returns early; the probe runs (and here is refused).
    [Fact]
    public async Task A_string_without_database_or_username_still_probes_and_throws_on_failure()
    {
        string sslMarker = "sslsecret-" + Guid.NewGuid().ToString("N");
        string connectionString = $"Host=127.0.0.1;Port=1;Password={PasswordValue};SSL Password={sslMarker};Timeout=5";

        Func<Task> act = () => PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(
            connectionString, BootstrapName, NullLogger.Instance);

        InvalidOperationException ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.InnerException.Should().BeAssignableTo<NpgsqlException>();
        ex.Message.Should().Contain("probe");
        ex.Message.Should().Contain(BootstrapName);
        ex.Message.Should().NotContain("Password");
        ex.Message.Should().NotContain(PasswordValue);
        ex.Message.Should().NotContain(sslMarker);
        ex.Message.Should().NotContain("127.0.0.1");
    }

    // Security S-21: Npgsql rejects "Min Pool Size"; the bootstrap drops it and reaches the probe.
    [Theory]
    [InlineData("Min Pool Size")]
    [InlineData("MinPoolSize")]
    [InlineData("Max Pool Size")]
    public async Task A_sqlclient_pool_size_spelling_does_not_stop_the_probe(string key)
    {
        string sslMarker = "sslsecret-" + Guid.NewGuid().ToString("N");

        Func<Task> act = () => PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(
            RefusedConnection(sslMarker, $";{key}=2"), BootstrapName, NullLogger.Instance);

        InvalidOperationException ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.InnerException.Should().BeAssignableTo<NpgsqlException>("the probe ran and was refused, not the parse");
        ex.Message.Should().Contain("probe");
    }
}
