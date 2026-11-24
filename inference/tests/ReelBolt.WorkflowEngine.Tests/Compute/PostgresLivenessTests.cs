using AwesomeAssertions;
using Npgsql;
using ReelBolt.Shared.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

public class PostgresLivenessTests
{
    private const string Plain = "Host=10.0.0.2;Port=5432;Database=reelforge;Username=u;Password=p";

    [Fact]
    public void AStringWithNoKeepAliveGetsProtocolAndTcpKeepAlives()
    {
        NpgsqlConnectionStringBuilder b = new(PostgresLiveness.Apply(Plain));

        b.KeepAlive.Should().Be(PostgresLiveness.KeepaliveSeconds);
        b.TcpKeepAlive.Should().BeTrue();
        b.TcpKeepAliveTime.Should().Be(PostgresLiveness.TcpKeepaliveTimeSeconds);
        b.TcpKeepAliveInterval.Should().Be(PostgresLiveness.TcpKeepaliveIntervalSeconds);
        b.Host.Should().Be("10.0.0.2");
        b.Password.Should().Be("p");
    }

    [Fact]
    public void AnOperatorsOwnValuesAreKept()
    {
        NpgsqlConnectionStringBuilder b = new(PostgresLiveness.Apply(Plain + ";Keepalive=60;Tcp Keepalive=false;Tcp Keepalive Time=90"));

        b.KeepAlive.Should().Be(60);
        b.TcpKeepAlive.Should().BeFalse();
        b.TcpKeepAliveTime.Should().Be(90);
        b.TcpKeepAliveInterval.Should().Be(PostgresLiveness.TcpKeepaliveIntervalSeconds);
    }
}
