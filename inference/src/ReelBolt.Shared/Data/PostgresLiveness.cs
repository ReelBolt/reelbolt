using Npgsql;

namespace ReelBolt.Shared.Data;

/// <summary>
/// Gives every Npgsql connection string TCP and protocol keep-alives, unless the string already sets them.
/// </summary>
/// <remarks>
/// The media box's engine reaches Postgres over the private network. When a connection through a published
/// port is cut without a FIN (a Docker network change flushes conntrack; a firewall reload does the same),
/// Npgsql's defaults leave it to the kernel: no TCP keep-alive on Linux for 7200 s and about 15 minutes of
/// retransmissions before a write fails. A run whose next database call landed on such a pooled connection sat
/// there for that long, with its lease still renewing on another connection (the 2026-11-17 CloudPool runs).
/// A short keep-alive finds a dead idle connection within ~45 s and evicts it from the pool before a step uses it.
/// </remarks>
public static class PostgresLiveness
{
    /// <summary>Seconds between protocol-level keep-alive probes of an idle pooled connection.</summary>
    public const int KeepaliveSeconds = 15;

    /// <summary>Idle seconds before the kernel starts probing; and the gap between probes (three are sent).</summary>
    public const int TcpKeepaliveTimeSeconds = 15;

    public const int TcpKeepaliveIntervalSeconds = 10;

    /// <summary>
    /// Returns the string with each liveness setting added when absent. A value the operator wrote is kept.
    /// </summary>
    public static string Apply(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        if (!HasKey(connectionString, "Keepalive"))
            builder.KeepAlive = KeepaliveSeconds;
        if (!HasKey(connectionString, "Tcp Keepalive") && !HasKey(connectionString, "TcpKeepAlive"))
            builder.TcpKeepAlive = true;
        if (!HasKey(connectionString, "Tcp Keepalive Time") && !HasKey(connectionString, "TcpKeepAliveTime"))
            builder.TcpKeepAliveTime = TcpKeepaliveTimeSeconds;
        if (!HasKey(connectionString, "Tcp Keepalive Interval") && !HasKey(connectionString, "TcpKeepAliveInterval"))
            builder.TcpKeepAliveInterval = TcpKeepaliveIntervalSeconds;
        return builder.ConnectionString;
    }

    // Whether the ORIGINAL string names a keyword (any spelling, any case): the builder cannot tell "unset" from
    // "set to the default" once parsed.
    private static bool HasKey(string connectionString, string keyword)
    {
        System.Data.Common.DbConnectionStringBuilder raw = new() { ConnectionString = connectionString };
        return raw.ContainsKey(keyword);
    }
}
